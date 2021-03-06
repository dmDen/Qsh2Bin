﻿#region Copyright (c) 2011-2015 Николай Морошкин, http://www.moroshkin.com/
/*

  Настоящий исходный код является частью приложения «Торговый привод QScalp»
  (http://www.qscalp.ru)  и  предоставлен  исключительно  в  ознакомительных
  целях.  Какое-либо коммерческое использование данного кода без письменного
  разрешения автора запрещено.

*/
#endregion

using System;
using QScalp.History.Internals;

namespace QScalp.History.Reader.V4
{
  sealed class OrdersStream : QshStream, IOrdersStream
  {
    // **********************************************************************

    public Security Security { get; private set; }
    public event Action<int, OwnOrder> Handler;

    // **********************************************************************

    public OrdersStream(DataReader dr)
      : base(StreamType.Orders, dr)
    {
      Security = new Security(dr.ReadString());
    }

    // **********************************************************************

    public override void Read(bool push)
    {
      OrderFlags flags = (OrderFlags)dr.ReadByte();
      OwnOrder order;

      if((flags & OrderFlags.DropAll) != 0)
        order = new OwnOrder();
      else
      {
        OwnOrderType type;

        if((flags & OrderFlags.Active) != 0)
        {
          if((flags & OrderFlags.Stop) != 0)
            type = OwnOrderType.Stop;
          else
            type = OwnOrderType.Regular;
        }
        else
          type = OwnOrderType.None;

        order = new OwnOrder(type, dr.ReadLeb128(),
          (int)dr.ReadLeb128(), (int)dr.ReadLeb128(), null);
      }

      if(push && Handler != null)
        Handler(Security.Key, order);
    }

    // **********************************************************************
  }
}
